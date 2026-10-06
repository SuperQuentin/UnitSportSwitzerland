using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --portaldemo[,out.png]</c>: a small street laid out by hand, every door
/// open, to show the door portals where the real world rarely lines things up. With a path it
/// saves a shot per view and quits; without one it cycles the views in a window.
///
/// <para>
/// House A has a front and a back door, the back one 2.5 m along: from the street, on the
/// diagonal through both, you see through the house to a red car in the backyard (a portal seen
/// through a portal). House B, across the street, faces A: from inside B
/// you look out across the street and into A. C and D stand side by side (several portals at
/// once), and D has people in it (the occupancy cue in its windows). A figure walks in through
/// A's front door, drawn on both sides of the doorway as it crosses.
/// </para>
///
/// <para>
/// Nothing is streamed and no server is involved: buildings, doors and plans are made here, and
/// the real pieces do the rest (<see cref="BuildingMeshBuilder"/>, <see cref="InteriorMeshBuilder"/>,
/// <see cref="DoorLink"/>, <see cref="DoorPortals"/>, <see cref="DoorwayGhosts"/>).
/// </para>
/// </summary>
public partial class PortalDemo : Node3D
{
    private readonly string? _shot;
    // tile 0/0 lands on the world origin: world and tile-local coordinates are the same
    private readonly WorldOrigin _origin = new(0, 1000);
    private readonly List<DoorLink> _links = new();
    private readonly Dictionary<string, InteriorNode> _interiors = new();
    private Camera3D _camera = null!;
    private Node3D _walker = null!;
    private MeshInstance3D _walkerMesh = null!;
    private float _walkerZ = 99f, _phase;
    private double _t;
    private int _view = -1;

    private sealed record HouseSpec(string Name, Vector2 Center, float Width, float Depth, BuildingKind Kind,
        (float X, bool South)[] Doors, FurnitureType[] Furniture, float DoorWidth = 1.0f, float DoorHeight = 2.1f);

    private static readonly HouseSpec[] Houses =
    {
        // A: front door on the street, back door 2.5 m along: seen through both on the diagonal
        new("A", new Vector2(0, -12), 10, 8, BuildingKind.House, new[] { (0f, true), (2.5f, false) },
            new[] { FurnitureType.Sofa, FurnitureType.Shelf, FurnitureType.Plant, FurnitureType.Rug }),
        // B: across the street, facing A
        new("B", new Vector2(0, 12), 10, 8, BuildingKind.House, new[] { (0f, false) },
            new[] { FurnitureType.Table, FurnitureType.Wardrobe, FurnitureType.Tv, FurnitureType.Sink }),
        new("C", new Vector2(14, -12), 9, 8, BuildingKind.House, new[] { (0f, true) },
            new[] { FurnitureType.Sofa, FurnitureType.Tv, FurnitureType.Rug }),
        new("D", new Vector2(26, -12), 9, 8, BuildingKind.Apartment, new[] { (0f, true) },
            new[] { FurnitureType.Table, FurnitureType.Shelf, FurnitureType.Plant }),
        // E: a barn west of A, its wall-sized double door swung out (10 m of its 16, under the 6.2 m eave)
        new("E", new Vector2(-17, -12), 16, 10, BuildingKind.Agricultural, new[] { (0f, true) },
            new FurnitureType[0], DoorWidth: 10.0f, DoorHeight: 5.85f),
        // F: a garage west of the barn, its roll-up door up in the lintel
        new("F", new Vector2(-31, -12), 3.6f, 6.4f, BuildingKind.Garage, new[] { (0f, true) },
            new[] { FurnitureType.Shelf }, DoorWidth: 2.8f, DoorHeight: 2.35f),
    };

    public PortalDemo(string? shot) => _shot = shot;

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--portaldemo");

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.9f, 0.9f, 0.9f),
            },
        });

        var buildingMaterial = World(Styles.MaterialRole.Building);
        var groundMaterial = World(Styles.MaterialRole.Terrain);
        var propMaterial = World(Styles.MaterialRole.Prop);
        var interiorMaterial = Styles.StyleKit.Material(Styles.MaterialRole.Interior);

        AddChild(Ground(groundMaterial));
        AddChild(Props(propMaterial));

        // the buildings and their doors, as a tile would bring them
        var tile = new BuildingTile { Id = new TileId(0, 0), Buildings = new List<Building>() };
        var spots = new List<DoorSpot>();
        foreach (var h in Houses)
        {
            int index = tile.Buildings.Count;
            tile.Buildings.Add(new Building
            {
                Kind = h.Kind, Floors = 2, MinY = 0, MaxY = 8.5f,
                Triangles = Solid(h.Center, h.Width, h.Depth, 6.2f, 8.5f),
            });
            foreach (var (x, south) in h.Doors)
            {
                float z = h.Center.Y + (south ? h.Depth / 2 + 0.03f : -h.Depth / 2 - 0.03f);
                spots.Add(new DoorSpot(index, new Vector3(h.Center.X + x, 0, z), new Vector3(0, 0, south ? 1 : -1), h.DoorWidth, h.DoorHeight));
            }
        }
        var mesh = BuildingMeshBuilder.Build(tile, spots.ToArray())!;
        AddChild(new MeshInstance3D { Name = "Buildings", Mesh = ChunkNode.ToArrayMesh(mesh, buildingMaterial) });

        // D has people in it: more lit windows, figures behind the glass
        var d = Houses[3];
        buildingMaterial.SetShaderParameter("occupied_box", new[] { new Vector4(d.Center.X, d.Center.Y, d.Width / 2, d.Depth / 2) });
        buildingMaterial.SetShaderParameter("occupied_axis", new[] { new Vector4(1, 0, 1, 0) });
        buildingMaterial.SetShaderParameter("occupied_count", 1);

        // the interiors, and a link per door, all open
        for (int i = 0; i < Houses.Length; i++)
        {
            var layout = Plan(Houses[i], i);
            var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), interiorMaterial,
                InteriorManager.PlacementFor(layout, _origin));
            AddChild(node);
            _interiors[layout.Key] = node;
            foreach (var e in layout.Entrances)
            {
                var link = DoorLink.Create(layout, e, _origin, Houses[i].DoorWidth, Houses[i].DoorHeight);
                link.Open = true;
                link.Swing = 1f;
                link.Leaf = node.Leaf(e.Door);
                link.Shutter = node.Shutter(e.Door);
                if (link.Leaf == null && DoorLeaf.OnFacade(link.Hang))
                {
                    link.Leaf = DoorLeaf.CreateOnFacade(e.Door, link.Outside, link.OutsideWidth, link.OutsideHeight,
                        link.Hang, layout.DressedKind(), interiorMaterial);
                    AddChild(link.Leaf);
                }
                link.SetLeaves(1f);
                _links.Add(link);
            }
        }

        var portals = new DoorPortals(() => _links, PlanAt)
        {
            Name = "Portals",
            OpenDoors = (boxes, axes, count) =>
            {
                buildingMaterial.SetShaderParameter("open_door_box", boxes);
                buildingMaterial.SetShaderParameter("open_door_axis", axes);
                buildingMaterial.SetShaderParameter("open_door_count", count);
            },
        };
        AddChild(portals);
        foreach (var l in _links) portals.Attach(l);
        AddChild(new DoorwayGhosts(() => _links, PlanAt) { Name = "Ghosts" });

        _camera = new Camera3D { Name = "Camera", Fov = 70, Far = 4000 };
        AddChild(_camera);
        _camera.MakeCurrent();

        // someone who walks in through A's front door
        _walker = new Node3D { Name = "Walker" };
        _walkerMesh = new MeshInstance3D { MaterialOverride = Avatar.HumanMeshBuilder.FigureMaterial() };
        _walker.AddChild(_walkerMesh);
        _walker.AddToGroup(DoorwayGhosts.Group);
        AddChild(_walker);
        _walker.Visible = false;
    }

    /// <summary>
    /// One gabled house per <see cref="BuildingKind"/>, 10 x 8 m with a door, as a tile brings it,
    /// for the model viewer (--models): the facade each kind gets.
    /// </summary>
    [Showcase("Terrain", "Building")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseBuildings() =>
        // a building under construction has no facade: it is drawn as its site (#608)
        Enum.GetValues<BuildingKind>().Where(kind => kind != BuildingKind.UnderConstruction).Select(kind => (kind.ToString(), (Func<Node3D>)(() =>
        {
            var tile = new BuildingTile
            {
                Id = new TileId(0, 0),
                Buildings = new List<Building>
                {
                    new() { Kind = kind, Floors = 2, MinY = 0, MaxY = 8.5f, Triangles = Solid(Vector2.Zero, 10f, 8f, 6.2f, 8.5f) },
                },
            };
            var door = new DoorSpot(0, new Vector3(0, 0, 4.03f), new Vector3(0, 0, 1), 1.1f, 2.2f);
            return new MeshInstance3D { Mesh = ChunkNode.ToArrayMesh(BuildingMeshBuilder.Build(tile, new[] { door })!, World(Styles.MaterialRole.Building)) };
        })));

    /// <summary>
    /// A block of flats' underground garage door (#558) with each road link: the pavement with its
    /// bollards and dropped kerb, the access road flaring into a T. A road's own surface is not
    /// drawn here as the game draws it; a dark strip stands in for the road the link meets.
    /// </summary>
    [Showcase("Terrain", "Garage door")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseGarageDoors() =>
        new[] { (LinkKind.Sidewalk, 5f), (LinkKind.Stub, 14f) }.Select(k => (k.Item1.ToString(), (Func<Node3D>)(() =>
        {
            var tile = new BuildingTile
            {
                Id = new TileId(0, 0),
                Buildings = new List<Building>
                {
                    new() { Kind = BuildingKind.Apartment, Floors = 5, MinY = 0, MaxY = 15f, Triangles = Solid(Vector2.Zero, 24f, 14f, 15f, 15.5f) },
                },
            };
            var door = new DoorSpot(0, new Vector3(6f, 0, 7.03f), new Vector3(0, 0, 1), GarageRule.Width, GarageRule.Height)
            {
                Slot = 1, Hang = DoorHang.RollUp, Vehicle = true,
                Link = new GarageLink(k.Item1, k.Item2, 0f, new Vector2(1, 0)),
            };
            var node = new Node3D();
            node.AddChild(new MeshInstance3D { Mesh = ChunkNode.ToArrayMesh(BuildingMeshBuilder.Build(tile, new[] { door })!, World(Styles.MaterialRole.Building)) });
            // the road it meets, so the link has something to join
            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(40f, 0.02f, 5f) },
                Position = new Vector3(6f, -0.01f, 7.03f + k.Item2 + 2.5f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.2f, 0.21f) },
            });
            return node;
        })));

    private static ShaderMaterial World(Styles.MaterialRole role)
    {
        var m = Styles.StyleKit.Material(role);
        FogUniforms.Apply(m);
        return m;
    }

    private string? PlanAt(Vector3 at) => InteriorNode.PlanAt(_interiors.Values, at);

    // ---- the views ------------------------------------------------------------------------------

    private readonly (string Name, double Seconds)[] _views =
    {
        ("two_houses", 2.0),     // C and D side by side, both open
        ("through", 2.0),        // through A's front door, its room, its back door, the backyard
        ("inside_out", 2.0),     // from inside B, across the street into A
        ("in_doorway", 2.0),     // lens 5 cm in front of A's facade: snapped out of the doorway
        ("in_reveal", 2.0),      // lens 10 cm inside A's doorway, looking out
        ("in_reveal_down", 2.0), // lens on A's doorway plane from inside, looking out and down (#78)
        ("barn", 2.0),           // E's wall-sized double door, both leaves swung out
        ("barn_swinging", 2.0),  // the same, half open
        ("barn_shut", 2.0),      // and shut: the pair over the facade's baked door, no flicker
        ("barn_inside", 2.0),    // from inside E, out through its door at the leaves
        ("barn_inside_shut", 2.0), // and shut: the pair's inner face, not a hole
        ("garage", 2.0),         // F's roll-up door, up in the lintel
        ("garage_rolling", 2.0), // half down
        ("garage_shut", 2.0),    // and down: the leaf over the facade's baked door, no flicker
        ("garage_inside", 2.0),  // from inside F, out through its door
        ("garage_inside_shut", 2.0), // and shut: its slats from inside, not a hole
        ("mirror", 2.0),         // in front of B's washbasin: its wall mirror (#439)
        ("crossing", 4.0),       // the figure walks in through A's front door
    };

    public override void _Process(double delta)
    {
        _t += delta;
        AnimateWalker((float)delta);
        int view = (int)Math.Floor(Progress(_t, out double into));
        if (view >= _views.Length)
        {
            if (_shot != null) { GetTree().Quit(0); return; }
            _t = 0;
            view = 0;
        }
        if (view != _view)
        {
            _view = view;
            Aim(_views[view].Name);
        }
        // the picture settles for a second (the portal viewports, the sizes), then the shot
        if (_shot != null && _views[view].Name != "crossing" && into > 1.2 && !_saved.Contains(view))
            Save(_views[view].Name, view);
    }

    private readonly HashSet<int> _saved = new();
    private bool _savedCrossing, _savedInside;

    private double Progress(double t, out double into)
    {
        for (int i = 0; i < _views.Length; i++)
        {
            if (t < _views[i].Seconds) { into = t; return i; }
            t -= _views[i].Seconds;
        }
        into = 0;
        return _views.Length;
    }

    private void Save(string name, int view)
    {
        _saved.Add(view);
        if (_shot == null) return;
        if (GetNodeOrNull<DoorPortals>("Portals") is { } portals)
            GD.Print($"[portaldemo] {name}: " + string.Join(", ", _links.Select(l =>
                $"{l.Door}{(portals.IsShown(l) ? " shown" : "")}{(portals.IsShownThrough(l) ? " through" : "")}")));
        foreach (var port in new[] { "Portals/Portal0", "Portals/Portal0/Portal0Through", "Portals/Portal1", "Portals/Portal1/Portal1Through" })
            if (GetNodeOrNull<SubViewport>(port) is { } vp && vp.RenderTargetUpdateMode == SubViewport.UpdateMode.Always)
                vp.GetTexture().GetImage()?.SavePng(_shot.Replace(".png", $"_{name}_{port.Replace('/', '-')}.png"));
        var path = _shot.Replace(".png", $"_{name}.png");
        if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) GD.Print($"[portaldemo] wrote {path}");
    }

    private void Aim(string view)
    {
        var a = _links.First(l => l.Plan == "0_0_0" && l.Outside.Basis.Z.Z > 0);
        var b = _links.First(l => l.Plan == "0_0_1");
        var barn = _links.First(l => l.Plan == "0_0_4");
        Transform3D Look(Vector3 eye, Vector3 at) => Transform3D.Identity.Translated(eye).LookingAt(at, Vector3.Up);
        var garage = _links.First(l => l.Plan == "0_0_5");
        barn.SetLeaves(view == "barn_swinging" ? 0.45f : view is "barn_shut" or "barn_inside_shut" ? 0f : 1f);
        garage.SetLeaves(view == "garage_rolling" ? 0.45f : view is "garage_shut" or "garage_inside_shut" ? 0f : 1f);
        switch (view)
        {
            case "garage":
            case "garage_rolling":
            case "garage_shut":
                _camera.GlobalTransform = Look(new Vector3(-27.5f, 1.7f, -1.5f), new Vector3(-31f, 1.3f, -8.8f));
                break;
            case "garage_inside":
            case "garage_inside_shut":
                _camera.GlobalTransform = Look(garage.Inside * new Vector3(0.6f, 1.6f, -4.5f), garage.Inside * new Vector3(0, 1.2f, 2f));
                break;
            case "barn":
            case "barn_swinging":
            case "barn_shut":
                _camera.GlobalTransform = Look(new Vector3(-7.5f, 2.2f, 9f), new Vector3(-17f, 3f, -7f));
                break;
            case "barn_inside":
            case "barn_inside_shut":
                _camera.GlobalTransform = Look(barn.Inside * new Vector3(1.2f, 1.7f, -5f), barn.Inside * new Vector3(0, 2f, 2f));
                break;
            case "mirror":
                if (GetTree().GetFirstNodeInGroup(WallMirror.Group) is WallMirror m)
                    _camera.GlobalTransform = Look(m.GlobalTransform * new Vector3(0.35f, 0.05f, 1.3f), m.GlobalPosition);
                break;
            case "two_houses":
                _camera.GlobalTransform = Look(new Vector3(20.5f, 1.7f, 4.5f), new Vector3(20f, 1.3f, -8f));
                break;
            case "through":
                // on the line from the front door through the back door, which ends at the red car
                _camera.GlobalTransform = Look(new Vector3(-1.9f, 1.7f, -2f), new Vector3(4.4f, 1.2f, -22f));
                break;
            case "inside_out":
            {
                var eye = b.Inside * new Vector3(0.1f, 1.6f, -1.5f);
                var at = b.ToInside * (a.Outside * new Vector3(0, 1.2f, 0));
                _camera.GlobalTransform = Look(eye, at);
                break;
            }
            case "in_doorway":
                _camera.GlobalTransform = a.Outside * Look(new Vector3(0.1f, 1.6f, 0.05f), new Vector3(0, 1.4f, -4f));
                break;
            case "in_reveal":
                _camera.GlobalTransform = a.Inside * Look(new Vector3(0.1f, 1.6f, -0.1f), new Vector3(0, 1.4f, 4f));
                break;
            case "in_reveal_down":
            {
                var eye = new Vector3(0.023f, 1.68f, -0.002f);
                _camera.GlobalTransform = a.Inside * Look(eye, eye + new Vector3(-0.27f, -0.64f, 0.72f));
                break;
            }
            case "crossing":
                _camera.GlobalTransform = Look(new Vector3(2.2f, 2.4f, -2.6f), new Vector3(0f, 1.1f, -8.6f));
                _walkerZ = -4.2f;
                _walker.Visible = true;
                _savedCrossing = _savedInside = false;
                break;
        }
    }

    /// <summary>The figure walks north into A: in the street, then, past the facade, carried inside by the door's map.</summary>
    private void AnimateWalker(float dt)
    {
        if (!_walker.Visible) return;
        const float speed = 1.3f;
        _walkerZ -= speed * dt;
        _phase = Avatar.HumanMeshBuilder.AdvancePhase(_phase, speed, dt);
        // one mesh rebuilt in place, not a new ArrayMesh per frame (#221)
        _walkerMesh.Mesh = Avatar.HumanMeshBuilder.BuildStride(Avatar.HumanPalette.Default, speed, _phase,
            into: _walkerMesh.Mesh as ArrayMesh ?? new ArrayMesh());
        var a = _links.First(l => l.Plan == "0_0_0" && l.Outside.Basis.Z.Z > 0);
        // facing north: the figure is authored facing +Z
        var street = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(0.15f, 0, _walkerZ));
        float facade = a.Outside.Origin.Z;
        _walker.GlobalTransform = _walkerZ > facade ? street : a.ToInside * street;

        if (_shot == null) { if (_walkerZ < facade - 2.5f) _walker.Visible = false; return; }
        // one shot with the body across the sill, one once it is in
        if (!_savedCrossing && _walkerZ < facade + 0.05f)
        {
            _savedCrossing = true;
            Save("crossing", 100);
        }
        if (!_savedInside && _walkerZ < facade - 1.2f)
        {
            _savedInside = true;
            Save("walked_in", 101);
            _walker.Visible = false;
        }
    }

    // ---- the street -------------------------------------------------------------------------------

    /// <summary>A house: walls to the eave, gables east and west, a pitched roof with its ridge along X.</summary>
    private static float[] Solid(Vector2 c, float w, float d, float eave, float ridge)
    {
        float x0 = c.X - w / 2, x1 = c.X + w / 2, z0 = c.Y - d / 2, z1 = c.Y + d / 2;
        var t = new List<float>();
        // wound clockwise seen from outside: Godot's front face, and the facade shader culls the back
        void Tri(Vector3 a, Vector3 b, Vector3 e) { t.AddRange(new[] { a.X, a.Y, a.Z, e.X, e.Y, e.Z, b.X, b.Y, b.Z }); }
        void Quad(Vector3 a, Vector3 b, Vector3 e, Vector3 f) { Tri(a, b, e); Tri(a, e, f); }
        Quad(new(x0, 0, z1), new(x1, 0, z1), new(x1, eave, z1), new(x0, eave, z1)); // south
        Quad(new(x1, 0, z0), new(x0, 0, z0), new(x0, eave, z0), new(x1, eave, z0)); // north
        Quad(new(x0, 0, z0), new(x0, 0, z1), new(x0, eave, z1), new(x0, eave, z0)); // west
        Tri(new(x0, eave, z0), new(x0, eave, z1), new(x0, ridge, c.Y));
        Quad(new(x1, 0, z1), new(x1, 0, z0), new(x1, eave, z0), new(x1, eave, z1)); // east
        Tri(new(x1, eave, z1), new(x1, eave, z0), new(x1, ridge, c.Y));
        Quad(new(x0, eave, z1), new(x1, eave, z1), new(x1, ridge, c.Y), new(x0, ridge, c.Y)); // roof
        Quad(new(x1, eave, z0), new(x0, eave, z0), new(x0, ridge, c.Y), new(x1, ridge, c.Y));
        return t.ToArray();
    }

    /// <summary>A one-room ground floor, a door per door, windows on the gable walls, some furniture.</summary>
    private static InteriorLayout Plan(HouseSpec h, int index)
    {
        // front (local -Z) toward the first door: turned round for a door on the south side
        bool south = h.Doors[0].South;
        float yaw = south ? Mathf.Pi : 0f;
        var turn = new Basis(Vector3.Up, yaw);
        float hw = h.Width / 2, hd = h.Depth / 2;
        var room = new RoomPlan { X0 = -hw, Z0 = -hd, X1 = hw, Z1 = hd, Type = RoomType.Living };
        var layout = new InteriorLayout
        {
            Key = $"0_0_{index}", Kind = h.Kind, TriangleCount = 0, MinY = 0, MaxY = 8.5f,
            CenterX = h.Center.X, CenterZ = h.Center.Y, Yaw = yaw, Width = h.Width, Depth = h.Depth,
            StoreyHeight = Math.Max(3.0f, h.DoorHeight + 0.4f), EntryWidth = h.DoorWidth,
        };
        int n = 0;
        foreach (var (x, doorSouth) in h.Doors)
        {
            var world = new Vector3(h.Center.X + x, 0, h.Center.Y + (doorSouth ? hd : -hd));
            var local = turn.Inverse() * (world - new Vector3(h.Center.X, 0, h.Center.Y));
            bool front = local.Z < 0;
            room.Openings.Add(new OpeningPlan
            {
                Side = front ? Side.Front : Side.Back, Center = local.X, Width = h.DoorWidth, Bottom = 0, Top = h.DoorHeight,
                Kind = OpeningKind.Entry,
            });
            var outward = new Vector3(0, 0, doorSouth ? 1 : -1);
            layout.Entrances.Add(new EntrancePlan
            {
                // the back door is slot 1 of the same building, as a real second door is (#498)
                Door = new DoorKey(0, 0, index, n++).ToString(), X = local.X, Z = front ? -hd : hd,
                InX = 0, InZ = front ? 1 : -1, Width = h.DoorWidth,
                DoorX = world.X, DoorY = 0, DoorZ = world.Z + outward.Z * 0.03f,
                DoorOutX = 0, DoorOutZ = outward.Z, DoorWidth = h.DoorWidth, DoorHeight = h.DoorHeight,
            });
        }
        var main = layout.Entrances[0];
        layout.DoorX = main.DoorX; layout.DoorY = main.DoorY; layout.DoorZ = main.DoorZ;
        layout.DoorOutX = main.DoorOutX; layout.DoorOutZ = main.DoorOutZ;
        layout.DoorWidth = h.DoorWidth; layout.DoorHeight = h.DoorHeight;
        layout.EntryX = main.X;

        foreach (var side in new[] { Side.Left, Side.Right })
            foreach (float z in new[] { -hd / 2, hd / 2 })
                room.Openings.Add(new OpeningPlan { Side = side, Center = z, Width = 1.2f, Bottom = 0.9f, Top = 2.2f, Kind = OpeningKind.Window });
        layout.Floors.Add(new FloorPlan { Rooms = { room } });

        // along the side walls, clear of the line between the doors
        float[] zs = { -hd + 1.4f, 0f, hd - 1.4f };
        for (int i = 0; i < h.Furniture.Length; i++)
        {
            var type = h.Furniture[i];
            bool left = i % 2 == 0;
            var (w, dd, ht) = type switch
            {
                FurnitureType.Sofa => (2.0f, 0.9f, 0.85f),
                FurnitureType.Shelf => (1.6f, 0.4f, 1.9f),
                FurnitureType.Wardrobe => (1.4f, 0.6f, 2.0f),
                FurnitureType.Table => (1.6f, 0.9f, 0.75f),
                FurnitureType.Tv => (1.2f, 0.4f, 0.9f),
                FurnitureType.Rug => (2.0f, 1.4f, 0.02f),
                _ => (0.5f, 0.5f, 1.1f),
            };
            layout.Furniture.Add(new FurniturePlan
            {
                Type = type, Floor = 0, X = left ? -hw + 0.3f + dd / 2 : hw - 0.3f - dd / 2, Z = zs[i % zs.Length],
                Turns = left ? 1 : 3, W = w, D = dd, H = ht,
            });
        }
        return layout;
    }

    /// <summary>Grass, and a street down the middle, vertex coloured for the terrain shader.</summary>
    private static MeshInstance3D Ground(Material material)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Quad(float x0, float z0, float x1, float z1, float y, Color c)
        {
            c.A = 0; // no surface pattern
            foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z0), (x1, z1), (x0, z1) })
            {
                st.SetColor(c);
                st.AddVertex(new Vector3(x, y, z));
            }
        }
        var grass = new Color(0.30f, 0.44f, 0.20f).SrgbToLinear();
        var tar = new Color(0.36f, 0.36f, 0.37f).SrgbToLinear();
        for (int gx = -8; gx < 8; gx++)
            for (int gz = -8; gz < 8; gz++)
                Quad(gx * 12f, gz * 12f, gx * 12f + 12f, gz * 12f + 12f, -0.02f, grass);
        Quad(-96f, -3f, 96f, 3f, 0f, tar);
        return new MeshInstance3D { Name = "Ground", Mesh = st.Commit(), MaterialOverride = material };
    }

    /// <summary>The backyard behind A, so the view through it ends somewhere: a kiosk, trees, a red car.</summary>
    private static MeshInstance3D Props(Material material)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Box(Vector3 min, Vector3 max, Color c)
        {
            c.A = 0; // not a light
            Vector3 P(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 }, new[] { 2, 3, 7, 6 }, new[] { 0, 4, 5, 1 } };
            foreach (var f in faces)
                foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    st.SetColor(c);
                    st.AddVertex(P(f[k]));
                }
        }
        Color C(float r, float g, float b) => new Color(r, g, b).SrgbToLinear();
        // a kiosk with a striped awning, in line with A's two doors
        Box(new(-1.5f, 0, -27), new(1.5f, 2.4f, -24.5f), C(0.85f, 0.80f, 0.62f));
        Box(new(-1.8f, 2.4f, -24.9f), new(1.8f, 2.7f, -23.8f), C(0.80f, 0.15f, 0.12f));
        Box(new(-1.8f, 2.7f, -27.2f), new(1.8f, 2.9f, -24.3f), C(0.20f, 0.35f, 0.65f));
        // a red car
        Box(new(3.0f, 0.25f, -23f), new(4.8f, 1.0f, -19f), C(0.75f, 0.10f, 0.10f));
        Box(new(3.2f, 1.0f, -22f), new(4.6f, 1.55f, -19.8f), C(0.25f, 0.30f, 0.38f));
        // trees
        foreach (var (x, z) in new[] { (-6f, -22f), (6.5f, -28f), (-3.5f, -31f), (9f, -21f), (-10f, -27f) })
        {
            Box(new(x - 0.2f, 0, z - 0.2f), new(x + 0.2f, 1.6f, z + 0.2f), C(0.35f, 0.24f, 0.14f));
            Box(new(x - 1.4f, 1.6f, z - 1.4f), new(x + 1.4f, 4.6f, z + 1.4f), C(0.18f, 0.40f, 0.16f));
        }
        return new MeshInstance3D { Name = "Backyard", Mesh = st.Commit(), MaterialOverride = material };
    }
}
